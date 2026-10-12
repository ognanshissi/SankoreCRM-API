namespace Sankore.Modules.Administration.Features.ComplianceSettings.UpdateComplianceSettings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class UpdateComplianceSettingsHandler(
    AdministrationDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<UpdateComplianceSettingsCommand, Result>
{
    public async Task<Result> Handle(UpdateComplianceSettingsCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var settings = await db.ComplianceSettings.AsTracking().FirstOrDefaultAsync(ct);

        // Materialise on first write rather than seeding every tenant: the defaults are the
        // behaviour, so a tenant that never configures compliance needs no row at all.
        if (settings is null)
        {
            settings = Domain.ComplianceSettings.CreateDefault(
                tenant.CurrentTenantId, currentUser.Id, now);
            db.ComplianceSettings.Add(settings);
        }

        // Parsed after the validator has already accepted them, so these cannot fail — but parsed
        // here rather than in the command so the domain never sees a string it has to interpret.
        var regime = string.IsNullOrWhiteSpace(cmd.FatcaRegime)
            ? (FatcaRegime?)null
            : Enum.Parse<FatcaRegime>(cmd.FatcaRegime);
        var requiredAt = Enum.Parse<FatcaRequiredAt>(cmd.FatcaRequiredAt);

        var fatca = settings.ConfigureFatca(
            cmd.FatcaEnabled, regime, cmd.FatcaGiin, requiredAt, cmd.FatcaForCoHolders,
            currentUser.Id, now);
        if (fatca.IsFailure)
            return fatca;

        var pep = settings.ConfigurePep(
            cmd.PepEnabled, cmd.PepPostMandateMonths, currentUser.Id, now);
        if (pep.IsFailure)
            return pep;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
