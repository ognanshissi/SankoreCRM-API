namespace Sankore.Modules.Customers.Features.LegalEntities;

/// <summary>
/// Error codes owned by the LegalEntities slices that are not (yet) part of
/// <see cref="Sankore.Modules.Customers.Domain.CustomerErrors"/>.
/// <para>
/// US-M01-BE-18 needs <c>CLIENT_NOT_LEGAL_ENTITY</c> to reject a beneficial-owner
/// declaration aimed at an individual. The shared <c>CustomerErrors</c> catalogue is
/// owned by the foundation slice, so the constant lives here until it is consolidated
/// there — the wire value is the contract and must not change when it moves.
/// </para>
/// </summary>
internal static class LegalEntityErrorCodes
{
    /// <summary>The target client exists but is an individual, not a legal entity.</summary>
    public const string ClientNotLegalEntity = "CLIENT_NOT_LEGAL_ENTITY";
}
