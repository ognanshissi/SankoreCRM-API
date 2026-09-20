namespace Sankore.Modules.Leads.Features.SlaConfigs.CreateSlaConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateSlaConfigHandler(LeadsDbContext db)
    : IRequestHandler<CreateSlaConfigCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateSlaConfigCommand cmd, CancellationToken ct)
    {
        // At most one active SLA per (TenantId, AgencyId) combination
        var hasActive = await db.SlaConfigs.AnyAsync(
            s => s.TenantId == cmd.TenantId
              && s.AgencyId == cmd.AgencyId
              && s.IsActive, ct);

        if (hasActive)
            return Result.Fail<Guid>("ACTIVE_SLA_ALREADY_EXISTS");

        var config = SlaConfig.Create(
            tenantId:              cmd.TenantId,
            agencyId:              cmd.AgencyId,
            name:                  cmd.Name,
            firstContactDeadline:  cmd.FirstContactDeadline,
            qualificationDeadline: cmd.QualificationDeadline,
            followUpDeadline:      cmd.FollowUpDeadline,
            escalationDeadline:    cmd.EscalationDeadline);

        db.SlaConfigs.Add(config);
        await db.SaveChangesAsync(ct);

        return Result.Ok(config.Id);
    }
}
