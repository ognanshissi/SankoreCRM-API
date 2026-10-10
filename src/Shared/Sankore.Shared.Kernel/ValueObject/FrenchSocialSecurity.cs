namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// French social-security identifiers of an insured person — <b>declared, not active</b> (BNF-B-21,
/// phase 2, France).
///
/// <para>
/// It exists now because BNF-B-02 asks for the block to be present from the start, and because
/// adding a member to an owned type after the fact is a migration on every table that maps
/// <see cref="PersonIdentity"/>. Nothing in phase 1 writes it: the UEMOA zone has no NIR, and the
/// gate is the tenant's country.
/// </para>
///
/// <para>
/// <b>No validation here on purpose.</b> The NIR's rules — 13 digits plus a key of
/// <c>97 − (n mod 97)</c>, consistency with gender and birth date, the Corsican 2A/2B
/// substitution — are phase-2 work, and a half-written check that accepts an invalid NIR is worse
/// than no check: it would let one into the blind index that the dedup then trusts. Until BNF-B-21
/// is implemented, treat a non-null value here as unverified.
/// </para>
/// </summary>
/// <param name="Nir">13 digits plus a 2-digit key. Encrypted and blind-indexed when phase 2 lands.</param>
/// <param name="OrganismCode">9 digits identifying the caisse.</param>
/// <param name="AttachedToMemberId">
/// For a child with no NIR of their own, the household member whose NIR covers them. An opaque id:
/// the household lives in a different schema in each module that maps this type.
/// </param>
public sealed record FrenchSocialSecurity(
    string? Nir = null,
    string? OrganismCode = null,
    Guid? AttachedToMemberId = null)
{
    /// <summary>True when nothing has been collected — the phase-1 state for every person.</summary>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(Nir)
           && string.IsNullOrWhiteSpace(OrganismCode)
           && AttachedToMemberId is null;
}
