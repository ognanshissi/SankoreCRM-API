namespace Sankore.Modules.Customers.Features.LegalEntities.DeactivateLegalForm;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateLegalFormHandler(CustomersDbContext db)
    : IRequestHandler<DeactivateLegalFormCommand, Result>
{
    public async Task<Result> Handle(DeactivateLegalFormCommand cmd, CancellationToken ct)
    {
        var code = cmd.Code.Trim();

        var form = await db.LegalForms
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Code == code, ct);

        // An unknown code and an already-retired one answer the same thing: the code is not
        // part of the tenant's usable list.
        if (form is null || !form.IsActive)
            return Result.Fail(CustomerErrors.LegalFormUnknown);

        form.Deactivate();
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
