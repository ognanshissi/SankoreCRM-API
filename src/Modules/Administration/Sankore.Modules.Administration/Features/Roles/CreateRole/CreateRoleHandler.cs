using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.CreateRole;

internal sealed class CreateRoleHandler(
    AdministrationDbContext db,
    RoleManager<AppRole> roleManager,
    ICurrentUser currentUser
) : IRequestHandler<CreateRoleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateRoleCommand request, CancellationToken ct)
    {
        // Uniqueness is scoped per tenant: two different tenants may use the same role name.
        // System roles (TenantId == null) are also reserved and cannot be shadowed.
        var nameNormalized = request.Name.ToUpperInvariant();
        var exists = await db.Roles
            .AnyAsync(r => r.NormalizedName == nameNormalized
                        && (r.TenantId == null || r.TenantId == currentUser.TenantId), ct);
        if (exists)
            return Result.Fail<Guid>("ROLE_NAME_TAKEN: A role with that name already exists for this tenant.");

        var role = AppRole.CreateCustom(currentUser.TenantId, request.Name, request.Label);
        var identityResult = await roleManager.CreateAsync(role);
        if (!identityResult.Succeeded)
            return Result.Fail<Guid>(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        return Result.Ok(role.Id);
    }
}
