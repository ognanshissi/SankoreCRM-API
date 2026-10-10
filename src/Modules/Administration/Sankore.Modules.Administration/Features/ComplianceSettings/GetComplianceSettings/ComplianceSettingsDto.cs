namespace Sankore.Modules.Administration.Features.ComplianceSettings.GetComplianceSettings;

/// <summary>
/// The compliance screen's read model (BNF-B-10).
///
/// <para>
/// Separate from <c>PublicApi.ComplianceSettingsDto</c> on purpose: this one is the HTTP contract
/// the Angular client is generated from and carries screen concerns (<c>UpdatedAt</c>, the allowed
/// regimes to populate a select), while the PublicApi record is the inter-module one and carries
/// only what another module decides with. Merging them would make an API field change a
/// cross-module contract change.
/// </para>
/// </summary>
public sealed record ComplianceSettingsResponse(
    bool FatcaEnabled,
    string? FatcaRegime,
    string? FatcaGiin,
    string FatcaRequiredAt,
    bool FatcaForCoHolders,
    int PepPostMandateMonths,
    DateTimeOffset UpdatedAt,
    /// <summary>Accepted values for <c>FatcaRegime</c>, so the client does not hard-code the enum.</summary>
    IReadOnlyList<string> AllowedRegimes,
    /// <summary>Accepted values for <c>FatcaRequiredAt</c>.</summary>
    IReadOnlyList<string> AllowedRequiredAt);
