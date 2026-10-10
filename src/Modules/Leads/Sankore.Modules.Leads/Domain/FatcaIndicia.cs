namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// The seven indices of US status BNF-B-11 enumerates, as declared by the person.
///
/// <para>
/// A typed jsonb record rather than seven boolean columns: they are read as a SET — "is any of
/// them true" decides the status — and never filtered on individually. Seven nullable columns
/// nobody queries would be seven migrations' worth of schema for one question.
/// </para>
///
/// <para>
/// <b>Each one has to stay separately identifiable</b>, which is why this is not a single
/// "hasIndicia" flag: the evidence that CURES an index depends on which index it is (BNF-B-11 —
/// a non-US passport, a certificate of loss of nationality, or a written explanation), so the
/// compliance officer reviewing a cure has to know what was being cured.
/// </para>
/// </summary>
/// <param name="UsNationalityOrResidence">US citizenship, or US residence including a green card.</param>
/// <param name="BornInTheUnitedStates">Place of birth in the United States.</param>
/// <param name="UsAddressOrPoBox">A US postal address or a US PO box.</param>
/// <param name="UsPhoneNumber">A US telephone number.</param>
/// <param name="StandingOrderToUsAccount">A standing payment order to an account held in the US.</param>
/// <param name="PowerOfAttorneyWithUsAddress">
/// A power of attorney or signature authority granted to a person with a US address.
/// </param>
/// <param name="CareOfOrPosteRestanteAsSoleAddress">
/// An "in care of" or poste-restante address as the ONLY address on file — an index precisely
/// because it is what somebody uses when they have no address they want on record.
/// </param>
public sealed record FatcaIndicia(
    bool UsNationalityOrResidence = false,
    bool BornInTheUnitedStates = false,
    bool UsAddressOrPoBox = false,
    bool UsPhoneNumber = false,
    bool StandingOrderToUsAccount = false,
    bool PowerOfAttorneyWithUsAddress = false,
    bool CareOfOrPosteRestanteAsSoleAddress = false)
{
    /// <summary>A declaration with nothing ticked.</summary>
    public static FatcaIndicia None => new();

    /// <summary>
    /// Whether anything at all was declared. This single question is what moves the status from
    /// <see cref="FatcaStatus.NonUsPerson"/> to <see cref="FatcaStatus.IndiciaPending"/>.
    /// </summary>
    public bool Any =>
        UsNationalityOrResidence
        || BornInTheUnitedStates
        || UsAddressOrPoBox
        || UsPhoneNumber
        || StandingOrderToUsAccount
        || PowerOfAttorneyWithUsAddress
        || CareOfOrPosteRestanteAsSoleAddress;

    /// <summary>
    /// The indices that were declared, by name — what a compliance screen shows and what an
    /// audit row can carry without carrying the person.
    /// </summary>
    public IReadOnlyList<string> Declared()
    {
        var declared = new List<string>(7);

        if (UsNationalityOrResidence) declared.Add(nameof(UsNationalityOrResidence));
        if (BornInTheUnitedStates) declared.Add(nameof(BornInTheUnitedStates));
        if (UsAddressOrPoBox) declared.Add(nameof(UsAddressOrPoBox));
        if (UsPhoneNumber) declared.Add(nameof(UsPhoneNumber));
        if (StandingOrderToUsAccount) declared.Add(nameof(StandingOrderToUsAccount));
        if (PowerOfAttorneyWithUsAddress) declared.Add(nameof(PowerOfAttorneyWithUsAddress));
        if (CareOfOrPosteRestanteAsSoleAddress) declared.Add(nameof(CareOfOrPosteRestanteAsSoleAddress));

        return declared;
    }
}

/// <summary>
/// A person's FATCA status (BNF-B-11). **Computed, never entered** — see
/// <see cref="FatcaDeclaration.Status"/>.
/// </summary>
public enum FatcaStatus
{
    /// <summary>
    /// FATCA is not enabled for the tenant. Never STORED: it is the effective status a reader sees
    /// when the institution is not subject to FATCA, which depends on a tenant setting the
    /// declaration itself cannot see — see <see cref="FatcaDeclaration.EffectiveStatus"/>.
    /// </summary>
    NotApplicable,

    /// <summary>Nothing declared. Nothing further is owed.</summary>
    NonUsPerson,

    /// <summary>At least one index declared, documentation not yet provided.</summary>
    IndiciaPending,

    /// <summary>W-9 and a US TIN provided — the person is reportable.</summary>
    UsPerson,

    /// <summary>W-8BEN and supporting evidence provided — the indices are lifted.</summary>
    IndiciaCured,

    /// <summary>The person has refused to provide the documentation.</summary>
    Recalcitrant
}

/// <summary>
/// Which form was provided to resolve declared indices (BNF-B-11).
/// </summary>
public enum FatcaDocumentation
{
    /// <summary>Nothing yet. With indices declared, this is <see cref="FatcaStatus.IndiciaPending"/>.</summary>
    None,

    /// <summary>
    /// IRS form W-9 — the person declares themselves a US person. Requires a US TIN, which is why
    /// the aggregate refuses this documentation without one.
    /// </summary>
    FormW9,

    /// <summary>
    /// IRS form W-8BEN — the person declares themselves a non-US person. Requires supporting
    /// evidence appropriate to the index being cured, which the aggregate also refuses without.
    /// </summary>
    FormW8Ben,

    /// <summary>
    /// The person refused. Recorded rather than left pending for ever: a refusal is a reportable
    /// outcome with its own obligations, not an absence of one.
    /// </summary>
    Refused
}
