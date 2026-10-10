namespace Sankore.Modules.Leads.Features.CustomerLink.GetCustomerMatches;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetCustomerMatchesHandler(LeadsDbContext db, ICustomersModule customers)
    : IRequestHandler<GetCustomerMatchesQuery, Result<IReadOnlyList<CustomerMatchDto>>>
{
    public async Task<Result<IReadOnlyList<CustomerMatchDto>>> Handle(
        GetCustomerMatchesQuery query, CancellationToken ct)
    {
        var lead = await db.Leads
            .Where(l => l.Id == query.LeadId)
            .Select(l => new
            {
                l.TenantId, l.CustomerId, l.PhoneNumber, l.Email, l.LegacyEmailClear, l.NationalId
            })
            .FirstOrDefaultAsync(ct);

        if (lead is null)
            return Result.Fail<IReadOnlyList<CustomerMatchDto>>("LEAD_NOT_FOUND");

        // Clear values: M01 holds a different blind-index key and indexes them itself — see
        // ICustomersModule.FindMatchesAsync. LegacyEmailClear covers a lead whose e-mail the
        // protection backfill has not reached yet.
        var matches = await customers.FindMatchesAsync(
            lead.TenantId,
            lead.PhoneNumber,
            lead.Email ?? lead.LegacyEmailClear,
            lead.NationalId,
            ct);

        var isSingle = matches.Count == 1;

        return Result.Ok<IReadOnlyList<CustomerMatchDto>>(
        [
            .. matches.Select(m => new CustomerMatchDto(
                m.ClientId, m.ClientNumber, m.DisplayName, m.AgencyId, m.AdvisorUserId,
                m.Status, m.KycStatus, m.MatchedOn,
                IsExactSingleMatch: isSingle && CustomerMatchPolicy.IsAutoAttachable(m, matches.Count),
                AlreadyLinked:      lead.CustomerId == m.ClientId))
        ]);
    }
}
