namespace Sankore.Modules.Administration.Features.ComplianceSettings.GetComplianceSettings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetComplianceSettingsHandler(AdministrationDbContext db)
    : IRequestHandler<GetComplianceSettingsQuery, Result<ComplianceSettingsResponse>>
{
    private static readonly IReadOnlyList<string> Regimes = Enum.GetNames<FatcaRegime>();
    private static readonly IReadOnlyList<string> RequiredAtValues = Enum.GetNames<FatcaRequiredAt>();

    /// <summary>
    /// The tiers the auto-approval ceiling may take. Spelled from <c>KycRemotePolicyValues</c>
    /// rather than from M02's enum, which this assembly cannot reference — see that class for why
    /// the duplication is accepted.
    /// </summary>
    private static readonly IReadOnlyList<string> KycTiers = KycRemotePolicyValues.Tiers;

    public async Task<Result<ComplianceSettingsResponse>> Handle(
        GetComplianceSettingsQuery request, CancellationToken ct)
    {
        var settings = await db.ComplianceSettings.FirstOrDefaultAsync(ct);

        // No row yet: answer with the platform defaults rather than 404. The defaults ARE the
        // tenant's configuration until somebody changes them, and a 404 on "what is my FATCA
        // setting" would read as "unknown" for a question whose answer is "off".
        if (settings is null)
        {
            return Result.Ok(new ComplianceSettingsResponse(
                FatcaEnabled:         false,
                FatcaRegime:          null,
                FatcaGiin:            null,
                FatcaRequiredAt:      nameof(Domain.FatcaRequiredAt.Conversion),
                FatcaForCoHolders:    true,
                // ON for a tenant with no row. The screen must not read "vigilance désactivée"
                // for an institution that has simply never opened it.
                PepEnabled:           true,
                PepPostMandateMonths: Domain.ComplianceSettings.DefaultPepPostMandateMonths,
                // OFF for a tenant with no row, and the screen must say so plainly: an institution
                // that has never opened this page has not adopted machine approval.
                KycAutoApprovalEnabled:   false,
                KycAutoApprovalMaxTier:   KycRemotePolicyValues.SimplifiedTier,
                KycAutoApprovalSamplePct: Domain.ComplianceSettings.DefaultKycAutoApprovalSamplePct,
                UpdatedAt:            DateTimeOffset.UtcNow,
                AllowedRegimes:       Regimes,
                AllowedRequiredAt:    RequiredAtValues,
                AllowedKycTiers:      KycTiers));
        }

        return Result.Ok(new ComplianceSettingsResponse(
            settings.FatcaEnabled,
            settings.FatcaRegime?.ToString(),
            settings.FatcaGiin,
            settings.FatcaRequiredAt.ToString(),
            settings.FatcaForCoHolders,
            settings.PepEnabled,
            settings.PepPostMandateMonths,
            settings.KycAutoApprovalEnabled,
            settings.KycAutoApprovalMaxTier,
            settings.KycAutoApprovalSamplePct,
            settings.UpdatedAt,
            Regimes,
            RequiredAtValues,
            KycTiers));
    }
}
