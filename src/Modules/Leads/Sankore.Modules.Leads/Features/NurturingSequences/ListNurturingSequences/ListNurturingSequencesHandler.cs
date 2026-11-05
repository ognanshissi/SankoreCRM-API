namespace Sankore.Modules.Leads.Features.NurturingSequences.ListNurturingSequences;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
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
                    .ToList(),
                // A lead that left nurturing keeps an active row until its next step comes due: it is no longer on it.
                db.NurturingEnrollments.Count(e => e.SequenceId == s.Id
                    && e.Status == NurturingEnrollmentStatus.Active
                    && db.Leads.Any(l => l.Id == e.LeadId && l.Status == LeadStatus.Nurturing))))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<NurturingSequenceDto>>(sequences);
    }
}
