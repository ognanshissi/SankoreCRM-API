namespace Sankore.Modules.Leads.Features.ListConsents;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListConsentsHandler(LeadsDbContext db)
    : IRequestHandler<ListConsentsQuery, Result<IReadOnlyList<ConsentDto>>>
{
    public async Task<Result<IReadOnlyList<ConsentDto>>> Handle(
        ListConsentsQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<ConsentDto>>("LEAD_NOT_FOUND");

        var consents = await db.LeadConsents
            .Where(c => c.LeadId == query.LeadId)
            .OrderByDescending(c => c.GrantedAt)
            .Select(c => new ConsentDto(
                c.Id,
                c.LeadId,
                c.Type.ToString(),
                c.Channel.ToString(),
                c.Status.ToString(),
                c.GrantedAt,
                c.ProofReference,
                c.RecordedBy,
                c.WithdrawnAt,
                c.WithdrawnBy,
                c.WithdrawalReason))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ConsentDto>>(consents);
    }
}
