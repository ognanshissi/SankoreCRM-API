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
                UpdatedAt:            DateTimeOffset.UtcNow,
                AllowedRegimes:       Regimes,
                AllowedRequiredAt:    RequiredAtValues));
        }

        return Result.Ok(new ComplianceSettingsResponse(
            settings.FatcaEnabled,
            settings.FatcaRegime?.ToString(),
            settings.FatcaGiin,
            settings.FatcaRequiredAt.ToString(),
            settings.FatcaForCoHolders,
            settings.PepEnabled,
            settings.PepPostMandateMonths,
            settings.UpdatedAt,
            Regimes,
            RequiredAtValues));
    }
}
