namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalForm;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class CreateLegalFormHandler(
    CustomersDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<CreateLegalFormCommand, Result<CreateLegalFormResult>>
{
    public async Task<Result<CreateLegalFormResult>> Handle(
        CreateLegalFormCommand cmd, CancellationToken ct)
    {
        var code = cmd.Code.Trim();

        // ux_legal_forms_code is unique on (TenantId, Code): re-posting the same code is a
        // replay, so return the existing row rather than inventing an error code.
        var existing = await db.LegalForms
            .FirstOrDefaultAsync(f => f.Code == code, ct);

        if (existing is not null)
        {
            return Result.Ok(new CreateLegalFormResult(
                existing.Id, existing.Code, Created: false, existing.IsActive));
        }

        var form = LegalForm.Create(
            tenantId: currentUser.TenantId,
            code: code,
            label: cmd.Label.Trim(),
            displayOrder: cmd.DisplayOrder);

        db.LegalForms.Add(form);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new CreateLegalFormResult(
            form.Id, form.Code, Created: true, form.IsActive));
    }
}
