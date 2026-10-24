namespace Sankore.Modules.Leads.Features.Enrichment.GetCompleteness;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLeadCompletenessHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    LeadCompletenessCalculator calculator,
    Administration.PublicApi.IAdministrationModule administration)
    : IRequestHandler<GetLeadCompletenessQuery, Result<LeadCompletenessDto>>
{
    public async Task<Result<LeadCompletenessDto>> Handle(
        GetLeadCompletenessQuery query, CancellationToken ct)
    {
        // Include, and the calculator says why: a lead loaded without its household reads as
        // complete while owing a questionnaire per child.
        var lead = await db.Leads
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<LeadCompletenessDto>("LEAD_NOT_FOUND");

        // Ownership-scoped like the household and questionnaire reads. The report names a spouse
        // and each child by first name and says which of them still owes a health declaration —
        // less than the household read returns, but about the same people, so it gets the same
        // perimeter rather than a weaker one chosen by accident.
        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return Result.Fail<LeadCompletenessDto>(access.Error!);

        // The line the client-file opening will check, so the gauge and the refusal agree.
        var productLineId = await ProductLines.ProductLineLookup.ForClientFileAsync(
            db, administration, lead, query.ProjectId, ct);

        return Result.Ok(await calculator.ComputeAsync(lead, productLineId, ct));
    }
}
