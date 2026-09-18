namespace Sankore.Modules.Leads.Features.GetSlaBreaches;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetSlaBreachesHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<GetSlaBreachesQuery, Result<IReadOnlyList<SlaBreachDto>>>
{
    public async Task<Result<IReadOnlyList<SlaBreachDto>>> Handle(
        GetSlaBreachesQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Assignments still active (lead not converted/archived), first contact not yet recorded,
        // and SLA deadline has passed.
        var breachesQuery =
            from assignment in db.LeadAssignments
            join lead in db.Leads on assignment.LeadId equals lead.Id
            where assignment.FirstContactAt == null
               && assignment.SlaDeadline < now
               && lead.Status != LeadStatus.Converted
               && lead.Status != LeadStatus.Archived
               && lead.Status != LeadStatus.Lost
               && lead.Status != LeadStatus.Disqualified
            select new { assignment, lead };

        if (query.AgentId.HasValue)
            breachesQuery = breachesQuery.Where(x => x.assignment.AgentId == query.AgentId.Value);

        if (query.AgencyId.HasValue)
            breachesQuery = breachesQuery.Where(x => x.lead.AgencyId == query.AgencyId.Value);

        var rows = await breachesQuery
            .OrderBy(x => x.assignment.SlaDeadline)
            .ToListAsync(ct);

        var results = rows.Select(x => new SlaBreachDto(
            LeadId:      x.lead.Id,
            FullName:    x.lead.FullName,
            PhoneNumber: x.lead.PhoneNumber,
            AgentId:     x.assignment.AgentId,
            AssignmentId: x.assignment.Id,
            AssignedAt:  x.assignment.CreatedAt,
            SlaDeadline: x.assignment.SlaDeadline,
            BreachHours: Math.Round((now - x.assignment.SlaDeadline).TotalHours, 1)
        )).ToList();

        return Result.Ok<IReadOnlyList<SlaBreachDto>>(results);
    }
}
