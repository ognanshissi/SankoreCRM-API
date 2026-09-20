namespace Sankore.Modules.Leads.Features.SlaConfigs.GetSlaConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetSlaConfigHandler(LeadsDbContext db)
    : IRequestHandler<GetSlaConfigQuery, Result<SlaConfigDto>>
{
    public async Task<Result<SlaConfigDto>> Handle(
        GetSlaConfigQuery query, CancellationToken ct)
    {
        var config = await db.SlaConfigs
            .Where(s => s.Id == query.SlaConfigId)
            .Select(s => new SlaConfigDto(
                s.Id,
                s.AgencyId,
                s.Name,
                s.FirstContactDeadline,
                s.QualificationDeadline,
                s.FollowUpDeadline,
                s.EscalationDeadline,
                s.IsActive,
                s.CreatedAt))
            .FirstOrDefaultAsync(ct);

        return config is null
            ? Result.Fail<SlaConfigDto>("SLA_CONFIG_NOT_FOUND")
            : Result.Ok(config);
    }
}
