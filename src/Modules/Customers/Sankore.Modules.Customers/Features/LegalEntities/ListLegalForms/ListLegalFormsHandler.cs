namespace Sankore.Modules.Customers.Features.LegalEntities.ListLegalForms;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListLegalFormsHandler(CustomersDbContext db)
    : IRequestHandler<ListLegalFormsQuery, Result<IReadOnlyList<LegalFormDto>>>
{
    public async Task<Result<IReadOnlyList<LegalFormDto>>> Handle(
        ListLegalFormsQuery query, CancellationToken ct)
    {
        // Tenant isolation comes from the global query filter on LegalForm — this list is
        // per-tenant configuration, not a global reference table.
        var forms = await db.LegalForms
            .Where(f => query.IncludeInactive || f.IsActive)
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Code)
            .Select(f => new LegalFormDto(f.Id, f.Code, f.Label, f.IsActive, f.DisplayOrder))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<LegalFormDto>>(forms);
    }
}
