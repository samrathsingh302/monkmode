// MonkMode.Tests - B6 service-deletion resistance: the deny-DELETE SDDL surgery.
//
// `sc delete MONKMODE` (OpenService DELETE + DeleteService) would remove the
// enforcement service outright. B6 adds ONE deny ACE - Deny, right SD (=DELETE)
// only, principal BA (Built-in Administrators): (D;;SD;;;BA) - to the service
// object's DACL while a block is active, re-asserted each tick and removed at a
// genuine expiry (or by the `unblock --force` escape hatch).
//
// The live SD I/O (QueryServiceObjectSecurity / SetServiceObjectSecurity) touches
// the real service object and is covered by the smoke test, not here. What IS
// pure and unit-tested is the SDDL string surgery in ServiceSecurity:
//   - the deny ACE constants (a typo could fail to resist sc-delete, or - far
//     worse - deny a right that BRICKS the service; pinned, drift fails loudly);
//   - SddlHasDenyDelete / AddDenyDeleteAce / RemoveDenyDeleteAce truth tables;
//   - the brick-safety round-trip Remove(Add(x)) == x (teardown always undoes);
//   - CLI<->service parity (the two byte-identical copies must never diverge).
//
// Everything here is in-memory strings - no service object is ever touched.

namespace MonkMode.Tests;

public class ServiceSecurityConstantsTests
{
    // The exact ACE we insert. A drift here changes what we deny and to whom.
    [Fact]
    public void DenyDeleteAce_IsExactly_DenySdToBa()
    {
        Assert.Equal("(D;;SD;;;BA)", MonkMode.ServiceSecurity.DenyDeleteAce);
    }

    [Fact]
    public void AceType_IsDeny()
    {
        Assert.Equal("D", MonkMode.ServiceSecurity.DenyAceType);
    }

    // THE brick guard: we deny SD (DELETE) and ONLY SD. WD (WRITE_DAC) or WO
    // (WRITE_OWNER) here would lock even the owner out of the DACL and brick the
    // service - the one mistake this constant must never make.
    [Fact]
    public void DeniedRight_IsDeleteOnly_NeverWriteDacOrOwner()
    {
        Assert.Equal("SD", MonkMode.ServiceSecurity.DeleteRight);
        Assert.NotEqual("WD", MonkMode.ServiceSecurity.DeleteRight); // WRITE_DAC
        Assert.NotEqual("WO", MonkMode.ServiceSecurity.DeleteRight); // WRITE_OWNER
    }

    // Principal is Built-in Administrators. The service runs as SY (not a BA
    // member) so it is unaffected; the admin CLI is BA but keeps WRITE_DAC.
    [Fact]
    public void Principal_IsBuiltinAdministrators()
    {
        Assert.Equal("BA", MonkMode.ServiceSecurity.AdministratorsSid);
    }
}

public class SddlHasDenyDeleteTests
{
    // A realistic service DACL already carrying our deny ACE as the leading ACE.
    private const string WithDeny =
        "O:SYG:SYD:(D;;SD;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)";

    private const string WithoutDeny =
        "O:SYG:SYD:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)";

    [Fact]
    public void Present_InDacl_IsTrue()
    {
        Assert.True(MonkMode.ServiceSecurity.SddlHasDenyDelete(WithDeny));
    }

    [Fact]
    public void Absent_IsFalse()
    {
        Assert.False(MonkMode.ServiceSecurity.SddlHasDenyDelete(WithoutDeny));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsFalse(string? sddl)
    {
        Assert.False(MonkMode.ServiceSecurity.SddlHasDenyDelete(sddl!));
    }

    [Fact]
    public void NoDaclSection_IsFalse()
    {
        // Owner/group only, no D: section - never read as "has the deny".
        Assert.False(MonkMode.ServiceSecurity.SddlHasDenyDelete("O:SYG:SY"));
    }

    // The same token sitting in the SACL (S:) must NOT count as our DACL deny -
    // we only ever inspect the D: portion.
    [Fact]
    public void SameTokenInSaclOnly_IsFalse()
    {
        const string saclDecoy = "O:SYG:SYD:(A;;FA;;;SY)S:(D;;SD;;;BA)";
        Assert.False(MonkMode.ServiceSecurity.SddlHasDenyDelete(saclDecoy));
    }

    // A hand-lower-cased copy must still read as present, so the re-assert does
    // not needlessly rewrite (and Remove can still strip it).
    [Fact]
    public void CaseInsensitiveMatch()
    {
        const string lowered = "O:SYG:SYD:(d;;sd;;;ba)(A;;FA;;;SY)";
        Assert.True(MonkMode.ServiceSecurity.SddlHasDenyDelete(lowered));
    }
}

public class AddDenyDeleteAceTests
{
    // Inserted as the FIRST ACE (deny ACEs precede allow ACEs in a canonical
    // DACL) - immediately after "D:" and before the first existing "(".
    [Fact]
    public void InsertsAsLeadingAce()
    {
        const string input = "O:SYG:SYD:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;FA;;;BA)";
        const string expected = "O:SYG:SYD:(D;;SD;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;FA;;;BA)";
        Assert.Equal(expected, MonkMode.ServiceSecurity.AddDenyDeleteAce(input));
    }

    // DACL flags (PAI/AI/P...) sit between "D:" and the first ACE; the deny goes
    // AFTER the flags, still leading the ACE list.
    [Fact]
    public void InsertsAfterDaclFlags()
    {
        const string input = "O:SYG:SYD:PAI(A;;FA;;;SY)";
        const string expected = "O:SYG:SYD:PAI(D;;SD;;;BA)(A;;FA;;;SY)";
        Assert.Equal(expected, MonkMode.ServiceSecurity.AddDenyDeleteAce(input));
    }

    [Fact]
    public void Idempotent_AddingTwiceEqualsOnce()
    {
        const string input = "O:SYG:SYD:(A;;FA;;;SY)";
        string once = MonkMode.ServiceSecurity.AddDenyDeleteAce(input);
        string twice = MonkMode.ServiceSecurity.AddDenyDeleteAce(once);
        Assert.Equal(once, twice);
    }

    // No D: section: we never fabricate a DACL from nothing (that could drop
    // existing protections) - the input is returned untouched.
    [Fact]
    public void NoDaclSection_ReturnsUnchanged()
    {
        const string input = "O:SYG:SY";
        Assert.Equal(input, MonkMode.ServiceSecurity.AddDenyDeleteAce(input));
    }

    [Fact]
    public void Null_ReturnsNull()
    {
        Assert.Null(MonkMode.ServiceSecurity.AddDenyDeleteAce(null!));
    }

    // A flags-only / empty DACL (no ACEs at all): the leading deny can still be
    // inserted at the end of the (empty) DACL section.
    [Fact]
    public void EmptyDacl_InsertsTheDeny()
    {
        const string input = "O:SYG:SYD:";
        Assert.Equal("O:SYG:SYD:(D;;SD;;;BA)", MonkMode.ServiceSecurity.AddDenyDeleteAce(input));
    }
}

public class RemoveDenyDeleteAceTests
{
    [Fact]
    public void RemovesTheDeny_LeavingEverythingElseIntact()
    {
        const string input = "O:SYG:SYD:(D;;SD;;;BA)(A;;FA;;;SY)(A;;FA;;;BA)";
        const string expected = "O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)";
        Assert.Equal(expected, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input));
    }

    [Fact]
    public void Absent_ReturnsUnchanged()
    {
        const string input = "O:SYG:SYD:(A;;FA;;;SY)";
        Assert.Equal(input, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input));
    }

    [Fact]
    public void Null_ReturnsNull()
    {
        Assert.Null(MonkMode.ServiceSecurity.RemoveDenyDeleteAce(null!));
    }

    [Fact]
    public void CaseInsensitive_RemovesLoweredCopy()
    {
        const string input = "O:SYG:SYD:(d;;sd;;;ba)(A;;FA;;;SY)";
        const string expected = "O:SYG:SYD:(A;;FA;;;SY)";
        Assert.Equal(expected, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input));
    }

    // Must NOT strip an identical token that lives in the SACL - only the DACL
    // copy is ours.
    [Fact]
    public void DoesNotStripFromSacl()
    {
        const string input = "O:SYG:SYD:(A;;FA;;;SY)S:(D;;SD;;;BA)";
        Assert.Equal(input, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input));
    }

    // #8 (audit P3): teardown must strip EVERY deny-DELETE ACE, not just the
    // first. AddDenyDeleteAce never stacks a duplicate, but an attacker can
    // hand-add a second (D;;SD;;;BA) via `sc sdset` mid-block; if teardown left
    // one behind the service would stay DELETE-denied and `unblock --force` /
    // expiry could no longer delete it.
    [Fact]
    public void RemovesEveryDenyAce_NotJustTheFirst()
    {
        const string input = "O:SYG:SYD:(D;;SD;;;BA)(D;;SD;;;BA)(A;;FA;;;SY)(A;;FA;;;BA)";
        const string expected = "O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)";
        string cleaned = MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input);
        Assert.Equal(expected, cleaned);
        Assert.False(MonkMode.ServiceSecurity.SddlHasDenyDelete(cleaned)); // now deletable
    }

    // Duplicates need not be adjacent - the fixed-point loop clears them wherever
    // they sit in the DACL.
    [Fact]
    public void RemovesNonAdjacentDuplicates()
    {
        const string input = "O:SYG:SYD:(D;;SD;;;BA)(A;;FA;;;SY)(D;;SD;;;BA)(A;;FA;;;BA)";
        const string expected = "O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)";
        Assert.Equal(expected, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input));
    }

    // A lower-cased hand-edited duplicate is stripped too (case-insensitive).
    [Fact]
    public void RemovesMixedCaseDuplicates()
    {
        const string input = "O:SYG:SYD:(D;;SD;;;BA)(d;;sd;;;ba)(A;;FA;;;SY)";
        const string expected = "O:SYG:SYD:(A;;FA;;;SY)";
        Assert.Equal(expected, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input));
    }

    // Even when clearing multiple DACL denies, an identical token in the SACL is
    // never touched - the loop respects the D: section boundary on every pass.
    [Fact]
    public void RemovesAllDaclDenies_ButNeverTheSacl()
    {
        const string input = "O:SYG:SYD:(D;;SD;;;BA)(D;;SD;;;BA)(A;;FA;;;SY)S:(D;;SD;;;BA)";
        const string expected = "O:SYG:SYD:(A;;FA;;;SY)S:(D;;SD;;;BA)";
        Assert.Equal(expected, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(input));
    }
}

public class BrickSafetyRoundTripTests
{
    // THE teardown-undo / brick-safety proof: for any DACL without our ACE,
    // Remove(Add(x)) == x. Whatever we add at OnStart, stopMe()/the escape hatch
    // can always remove byte-for-byte, so an expired block always leaves a
    // removable service.
    [Theory]
    [InlineData("O:SYG:SYD:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;FA;;;BA)")]
    [InlineData("O:SYG:SYD:PAI(A;;FA;;;SY)")]
    [InlineData("O:SYG:SYD:(A;;FA;;;SY)S:(AU;FA;FA;;;WD)")]
    public void RemoveAfterAdd_RestoresOriginal(string original)
    {
        string added = MonkMode.ServiceSecurity.AddDenyDeleteAce(original);
        Assert.NotEqual(original, added); // sanity: it really did add
        Assert.Equal(original, MonkMode.ServiceSecurity.RemoveDenyDeleteAce(added));
    }
}

public class ServiceSecurityParityTests
{
    // The CLI copy (MonkMode.ServiceSecurity) and the service copy
    // (monkmode.ServiceSecurity) are byte-identical by policy. If one is edited
    // without the other, the service could deny an ACE the CLI's escape hatch no
    // longer knows how to remove - so pin them equal (mirrors the crypto /
    // canonical parity tests).
    [Fact]
    public void Constants_MatchAcrossCopies()
    {
        Assert.Equal(monkmode.ServiceSecurity.DenyDeleteAce, MonkMode.ServiceSecurity.DenyDeleteAce);
        Assert.Equal(monkmode.ServiceSecurity.DeleteRight, MonkMode.ServiceSecurity.DeleteRight);
        Assert.Equal(monkmode.ServiceSecurity.AdministratorsSid, MonkMode.ServiceSecurity.AdministratorsSid);
        Assert.Equal(monkmode.ServiceSecurity.DenyAceType, MonkMode.ServiceSecurity.DenyAceType);
    }

    [Theory]
    [InlineData("O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData("O:SYG:SYD:PAI(A;;FA;;;SY)")]
    [InlineData("O:SYG:SYD:(D;;SD;;;BA)(A;;FA;;;SY)")]
    [InlineData("O:SYG:SYD:(D;;SD;;;BA)(D;;SD;;;BA)(A;;FA;;;SY)")] // #8: doubled deny stripped identically by both copies
    [InlineData("O:SYG:SY")]
    public void Functions_AgreeAcrossCopies(string sddl)
    {
        Assert.Equal(
            monkmode.ServiceSecurity.SddlHasDenyDelete(sddl),
            MonkMode.ServiceSecurity.SddlHasDenyDelete(sddl));
        Assert.Equal(
            monkmode.ServiceSecurity.AddDenyDeleteAce(sddl),
            MonkMode.ServiceSecurity.AddDenyDeleteAce(sddl));
        Assert.Equal(
            monkmode.ServiceSecurity.RemoveDenyDeleteAce(sddl),
            MonkMode.ServiceSecurity.RemoveDenyDeleteAce(sddl));
    }
}
