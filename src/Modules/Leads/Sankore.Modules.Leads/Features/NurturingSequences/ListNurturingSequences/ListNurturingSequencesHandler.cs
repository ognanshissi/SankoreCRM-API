namespace Sankore.Modules.Leads.Features.NurturingSequences.ListNurturingSequences;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListNurturingSequencesHandler(LeadsDbContext db)
    : IRequestHandler<ListNurturingSequencesQuery, Result<IReadOnlyList<NurturingSequenceDto>>>
{
    public async Task<Result<IReadOnlyList<NurturingSequenceDto>>> Handle(
        ListNurturingSequencesQuery query, CancellationToken ct)
    {
        var q = db.NurturingSequences.AsQueryable();

        if (query.ActiveOnly == true)
            q = q.Where(s => s.IsActive);

        var sequences = await q
            .OrderBy(s => s.Name)
            .Select(s => new NurturingSequenceDto(
                s.Id,
                s.Name,
                s.Description,
                s.IsActive,
                s.CreatedAt,
                s.Steps.OrderBy(st => st.Order)
                    .Select(st => new NurturingStepDto(
                        st.Id, st.Order, st.DelayFromPrevious,
                        st.EmailTemplateKey, st.Subject))
                    .ToList()))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<NurturingSequenceDto>>(sequences);
    }
}
