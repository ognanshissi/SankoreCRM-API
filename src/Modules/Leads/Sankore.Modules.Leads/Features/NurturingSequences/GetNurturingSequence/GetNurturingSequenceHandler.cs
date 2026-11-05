namespace Sankore.Modules.Leads.Features.NurturingSequences.GetNurturingSequence;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetNurturingSequenceHandler(LeadsDbContext db)
    : IRequestHandler<GetNurturingSequenceQuery, Result<NurturingSequenceDto>>
{
    public async Task<Result<NurturingSequenceDto>> Handle(
        GetNurturingSequenceQuery query, CancellationToken ct)
    {
        var dto = await db.NurturingSequences
            .Where(s => s.Id == query.SequenceId)
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
            .FirstOrDefaultAsync(ct);

        return dto is null
            ? Result.Fail<NurturingSequenceDto>("NURTURING_SEQUENCE_NOT_FOUND")
            : Result.Ok(dto);
    }
}
