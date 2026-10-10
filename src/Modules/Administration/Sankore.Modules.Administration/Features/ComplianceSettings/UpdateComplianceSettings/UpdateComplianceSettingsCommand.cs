namespace Sankore.Modules.Administration.Features.ComplianceSettings.UpdateComplianceSettings;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Sets the tenant's compliance parameters (BNF-B-10). A full replacement, not a patch: the FATCA
/// regime, the GIIN and the enabled flag only mean anything together, and a PATCH that could carry
/// one of the three would let a caller enable FATCA in one request and name the institution in a
/// second — with the window between them spent collecting declarations nobody can report.
///
/// <para>
/// <c>ICommand</c> activates <c>TransactionBehavior</c> and <c>AuditBehavior</c>, and
/// <c>IResourceCommand</c> names the row in the audit trail: BNF-B-10 requires every change here
/// to be auditable, and "a compliance setting changed" is not useful without knowing whose.
/// </para>
/// </summary>
/// <param name="FatcaEnabled">
/// Enabling requires <paramref name="FatcaRegime"/> and a well-formed <paramref name="FatcaGiin"/>.
/// Disabling CLEARS both — a dormant GIIN is one a later re-enable would adopt silently.
/// </param>
/// <param name="FatcaRegime">"IgaModel1" | "IgaModel2" | "DirectFfiAgreement". Ignored when disabling.</param>
/// <param name="FatcaGiin">Format <c>XXXXXX.XXXXX.XX.XXX</c>. Ignored when disabling.</param>
/// <param name="FatcaRequiredAt">"Conversion" (default) | "Enrichment".</param>
/// <param name="FatcaForCoHolders">Collect indicia from co-holders too.</param>
/// <param name="PepPostMandateMonths">
/// Months a person stays politically exposed after their function ends. Not a FATCA field and not
/// disablable — PEP vigilance is required of every UEMOA institution.
/// </param>
public sealed record UpdateComplianceSettingsCommand(
    bool FatcaEnabled,
    string? FatcaRegime,
    string? FatcaGiin,
    string FatcaRequiredAt,
    bool FatcaForCoHolders,
    int PepPostMandateMonths) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ComplianceSettings";

    /// <summary>
    /// Null: the row is identified by the tenant, which the audit entry already carries, and the
    /// row's own id is a surrogate nobody can look up.
    /// </summary>
    public string? ResourceId => null;
}
